using DSPiConsole.Controls.GraphEditing;
using DSPiConsole.Core.GraphEditing;
using DSPiConsole.Core.Models;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;

namespace DSPiConsole.Controls;

/// <summary>
/// On-graph PEQ editing (see <see cref="PeqGraphEditor"/> for the behaviour).
/// The editor overlay covers the plot area and edits the channel whose page is
/// open, when all of these hold: a device is connected, the channel's curve is
/// visible, the page shows PEQ bands (not the XO tab), and, in the pop-out,
/// it follows the main window's selection. That channel's curve, dots and band
/// shapes are drawn by the overlay, so the plot's own curves leave it out.
/// </summary>
public sealed partial class BodePlotControl
{
    private PeqGraphEditorView? _editorView;
    /// <summary>The linked partner of the edited input, left out of the plot's
    /// curves while a graph edit is live, or -1. The model, which the plot draws
    /// from, only takes the edit on release, so the partner's curve would sit
    /// still until then; the overlay's live curve stands for both meanwhile.</summary>
    private int _livePartnerId = -1;
    /// <summary>The live spectrum, under the grid and the curves.</summary>
    private Rta.GraphSpectrumOverlay? _spectrum;
    private bool _editingSuspended;
    /// <summary>The channel the editor draws, or -1.</summary>
    private int _editedChannelId = -1;

    private void InitializeEditing()
    {
        // The overlay needs the view model as its host, so it is created once
        // the control has one (AttachEditor, from OnLoaded).
    }

    private void AttachEditor()
    {
        if (_editorView != null || _viewModel == null) return;
        _editorView = new PeqGraphEditorView(_viewModel);
        _rootGrid!.Children.Add(_editorView);
        _spectrum = new Rta.GraphSpectrumOverlay(_viewModel, followsGraphPreference: true);
        _rootGrid.Children.Insert(0, _spectrum);
        LayoutEditor();
        RefreshEditor(redraw: false);
    }

    private void OnGraphBandLive(int channel, int band, FilterParams p)
    {
        if (_viewModel == null || channel != _editedChannelId || _livePartnerId >= 0) return;
        if (!_viewModel.IsInputPairLinked(channel)) return;
        _livePartnerId = MainViewModel.GetLinkedInputChannel(channel);
        Redraw(gridChanged: true);
    }

    private void OnGraphLiveEnded(int channel)
    {
        if (_livePartnerId < 0) return;
        // The commit follows this call; once it has updated the targets, the
        // partner comes back already at its new curve rather than animating
        // there from the stale one.
        DispatcherQueue.TryEnqueue(() =>
        {
            int partner = _livePartnerId;
            if (partner < 0) return;
            _livePartnerId = -1;
            if (_targetMagnitudes.TryGetValue(partner, out var target) && _currentMagnitudes.TryGetValue(partner, out var current))
                Array.Copy(target, current, NumPoints);
            Redraw(gridChanged: true);
        });
    }

    /// <summary>Releases the spectrum's subscription for good. The pop-out
    /// calls it on close, since Unloaded is not reliably raised then.</summary>
    public void ReleaseSpectrum() => _spectrum?.Dispose();

    /// <summary>Suspends graph editing while the page below lists something
    /// other than PEQ bands (the XO tab).</summary>
    public void SetEditingSuspended(bool suspended)
    {
        if (_editingSuspended == suspended) return;
        _editingSuspended = suspended;
        RefreshEditor();
    }

    /// <summary>The editor overlay sits exactly over the plot rect, so its
    /// coordinates are the plot's.</summary>
    private void LayoutEditor()
    {
        if (_editorView == null) return;
        _editorView.Margin = new Thickness(LeftMargin, TopMargin, RightMargin, BottomMargin);
        if (_spectrum != null)
        {
            _spectrum.Margin = _editorView.Margin;
            _spectrum.SetAxis(MinFreq, MaxFreq);
        }
    }

    /// <summary>Hands the editor the current channel, bands and axes. With
    /// <paramref name="redraw"/>, also redraws the plot's curves when the edited
    /// channel changes, since the plot leaves that channel's curve out.</summary>
    private void RefreshEditor(bool redraw = true)
    {
        if (_editorView == null || _viewModel == null) return;
        var config = BuildEditorConfig();
        int edited = config.Channel ?? -1;
        bool changed = edited != _editedChannelId;
        _editedChannelId = edited;
        LayoutEditor();
        _editorView.Apply(config);
        if (changed && redraw) Redraw(gridChanged: true);
    }

    private PeqEditorConfig BuildEditorConfig()
    {
        var vm = _viewModel!;
        var settings = AppSettings.Instance;
        var axes = new PeqEditorConfig
        {
            MinFreq = MinFreq,
            MaxFreq = MaxFreq,
            DbTop = DbTop,
            DbBottom = DbBottom,
        };
        if (_selectedChannelId < 0 || _editingSuspended || _ignoreVisibility || !vm.IsDeviceConnected)
            return axes;
        var channel = Channel.FromId((ChannelId)_selectedChannelId);
        if (channel == null || !IsChannelVisible(channel)) return axes;

        var bands = vm.GetFilters(channel).Take(PeqGraphEditor.Tuning.BandRows).Select(b => b.Clone()).ToList();
        var statics = channel.IsOutput && vm.CrossoverSupported
            ? vm.GetXoverFilters(channel).Select(b => b.Clone()).ToList()
            : new List<FilterParams>();
        var available = FilterTypeExtensions.PeqTypes
            .Where(t => t != FilterType.Flat && t != FilterType.LinkwitzTransform && vm.FilterTypeSupported(t))
            .ToHashSet();

        return new PeqEditorConfig
        {
            Channel = _selectedChannelId,
            Bands = bands,
            Statics = statics,
            OffsetDb = EditLevelOffset(channel),
            // The master EQ bypass flattens the input curves (GetResponseCurve).
            Flat = vm.Bypass && channel.Id is ChannelId.MasterLeft or ChannelId.MasterRight,
            CurveColor = (channel.Color.R / 255f, channel.Color.G / 255f, channel.Color.B / 255f),
            LineWidth = (float)settings.GraphLineWidth,
            Glow = settings.ShowGraphGlow,
            MinFreq = MinFreq,
            MaxFreq = MaxFreq,
            DbTop = DbTop,
            DbBottom = DbBottom,
            AvailableTypes = available,
            BypassSupported = vm.BandBypassSupported,
            ShowFrequencyReadout = settings.ShowFrequencyReadout,
            ShowLevelReadout = settings.ShowGainReadout,
        };
    }

    /// <summary>The gain/preamp offset folded into the drawn curves when "Gain
    /// affects displayed level" is on, so the edited curve sits where the
    /// plot's other curves would draw it.</summary>
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
}
