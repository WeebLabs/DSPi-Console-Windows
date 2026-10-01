using DSPiConsole.Core.Models;
using DSPiConsole.Core.Rta;
using DSPiConsole.Models;
using Windows.UI;

namespace DSPiConsole.ViewModels;

/// <summary>
/// The spectrum analyser's place in the view model: the engine, and which
/// channels each page shows. The dashboard's selection is remembered; a channel
/// page starts on its own channel each time it opens, so leaving it returns the
/// dashboard to what it showed before. Port of the macOS Console's
/// DSPViewModel RTA extension.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The analyser engine. Its frames never pass through property
    /// notification; displays read <see cref="RtaEngine.Snapshot"/>.</summary>
    public RtaEngine Rta { get; }

    /// <summary>The engine's state (support, caps, options, layout) changed.
    /// Raised on the UI thread.</summary>
    public event EventHandler? RtaStateChanged;
    /// <summary>The engine's status changed. Raised on the UI thread.</summary>
    public event EventHandler? RtaTelemetryChanged;
    /// <summary>A page's selection, or where pages show their spectrum,
    /// changed. Raised on the UI thread.</summary>
    public event EventHandler? RtaSelectionChanged;

    /// <summary>The channel page that is open, or -1 for the dashboard.</summary>
    private int _rtaPageChannelId = -1;
    private RtaChannelSelection _rtaPageSelection = RtaChannelSelection.None;
    private RtaChannelSelection? _rtaPageOtherSide;

    private void InitializeRta()
    {
        Rta.StateChanged += () => _dispatcher.TryEnqueue(() => RtaStateChanged?.Invoke(this, EventArgs.Empty));
        Rta.TelemetryChanged += () => _dispatcher.TryEnqueue(() => RtaTelemetryChanged?.Invoke(this, EventArgs.Empty));
        Rta.SetOptions(RtaOptionsFromSettings);
    }

    /// <summary>The device-side options from the preferences, clamped to the
    /// sizes any device offers.</summary>
    public static RtaOptions RtaOptionsFromSettings
    {
        get
        {
            var s = AppSettings.Instance;
            return new RtaOptions(
                (byte)Math.Clamp(s.RtaFftOrder, RtaWire.OrderMin, RtaWire.OrderMax),
                (ushort)Math.Clamp(s.RtaAvgMs, 0, 10000),
                (byte)Math.Clamp(s.RtaPeakDecayDbS, 0, 100));
        }
    }

    public RtaScale RtaScale => new(AppSettings.Instance.RtaFloorDb, AppSettings.Instance.RtaCeilingDb);

    public bool RtaOnDashboard => _rtaPageChannelId < 0;

    /// <summary>"Dashboard", or the open channel page's name.</summary>
    public string RtaPageTitle =>
        RtaOnDashboard ? "Dashboard" : RtaChannelOf(_rtaPageChannelId) is var (tap, ch) ? RtaChannelName(tap, ch) : "Channel";

    /// <summary>Whether the open page draws its spectrum anywhere.</summary>
    public bool RtaPageShowsSpectrum =>
        AppSettings.Instance.RtaShows(bars: false, RtaOnDashboard) || AppSettings.Instance.RtaShows(bars: true, RtaOnDashboard);

    // ── Channels ──

    /// <summary>Channels the analyser can show at a tap: every live input row,
    /// or every enabled output. Clamped to what the caps report, since a mask
    /// bit for a channel the device lacks is a refused config.</summary>
    public IReadOnlyList<int> RtaChannels(byte tap)
    {
        int reported = tap == RtaWire.TapInput ? Rta.Caps.InputChannels : Rta.Caps.OutputChannels;
        int limit = Math.Min(reported > 0 ? reported : 16, 16);
        if (tap == RtaWire.TapInput)
            return Enumerable.Range(0, Math.Min(ActiveInputChannelCount, limit)).ToArray();
        return Enumerable.Range(0, ActiveOutputs.Count).Where(o => o < limit && IsOutputEnabled(o)).ToArray();
    }

    /// <summary>The app channel behind an analyser channel: an input row, or a
    /// matrix output in sidebar order.</summary>
    public Channel? RtaChannel(byte tap, int channel)
    {
        if (tap == RtaWire.TapInput)
            return channel >= 0 && channel < Channel.AllInputs.Count ? Channel.AllInputs[channel] : null;
        var outputs = ActiveOutputs;
        return channel >= 0 && channel < outputs.Count ? outputs[channel] : null;
    }

    public string RtaChannelName(byte tap, int channel) =>
        RtaChannel(tap, channel) is { } c ? GetChannelName(c) : $"Ch {channel + 1}";

    /// <summary>The channel's response-curve colour, so its spectrum can never
    /// end up a different colour from its own curve.</summary>
    public Color RtaChannelColor(byte tap, int channel) =>
        RtaChannel(tap, channel)?.Color ?? Color.FromArgb(255, 0, 120, 215);

    /// <summary>An app channel id as an analyser channel.</summary>
    public (byte Tap, int Channel)? RtaChannelOf(int channelId)
    {
        for (int i = 0; i < Channel.AllInputs.Count; i++)
            if ((int)Channel.AllInputs[i].Id == channelId) return (RtaWire.TapInput, i);
        var outputs = ActiveOutputs;
        for (int o = 0; o < outputs.Count; o++)
            if ((int)outputs[o].Id == channelId) return (RtaWire.TapOutput, o);
        return null;
    }

    // ── Selection ──

    /// <summary>The dashboard's selection as stored, limited to live channels.
    /// Never chosen, or every chosen channel gone, falls back to the first
    /// enabled output, then input 1. A stored empty selection stays empty: the
    /// user hid the spectrum.</summary>
    public RtaChannelSelection DashboardRtaSelection
    {
        get
        {
            if (RtaChannelSelection.FromStorageKey(AppSettings.Instance.RtaDashboardSelectionKey) is { } stored)
            {
                if (stored.IsEmpty) return stored;
                var live = stored.Restricted(RtaChannels(stored.Tap).ToArray());
                if (!live.IsEmpty) return live;
            }
            var outputs = RtaChannels(RtaWire.TapOutput);
            return outputs.Count > 0
                ? new RtaChannelSelection(RtaWire.TapOutput, new[] { outputs[0] })
                : new RtaChannelSelection(RtaWire.TapInput, new[] { 0 });
        }
    }

    /// <summary>The selection for whichever page is open.</summary>
    public RtaChannelSelection RtaSelection =>
        RtaOnDashboard ? DashboardRtaSelection : _rtaPageSelection.Restricted(RtaChannels(_rtaPageSelection.Tap).ToArray());

    public void SetRtaSelection(RtaChannelSelection selection)
    {
        var settings = AppSettings.Instance;
        if (RtaOnDashboard)
        {
            settings.RtaDashboardSelectionKey = selection.StorageKey;
        }
        else
        {
            _rtaPageSelection = selection;
            // Hiding the spectrum on one channel page keeps it hidden on the
            // next, rather than bringing it back every time a page opens.
            settings.RtaChannelPagesShowSpectrum = !selection.IsEmpty;
        }
        settings.Save();
        RtaSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Show the other side's channels, bringing back what was checked
    /// there when the user last left it. Not a hide, so it leaves the channel
    /// pages' show-spectrum preference alone.</summary>
    public void SwitchRtaSide(byte tap)
    {
        if (RtaOnDashboard)
        {
            var settings = AppSettings.Instance;
            // The stored form, so an output disabled for now is remembered.
            var active = RtaChannelSelection.FromStorageKey(settings.RtaDashboardSelectionKey) ?? DashboardRtaSelection;
            var next = RtaChannelSelection.SwitchingSides(active, RtaChannelSelection.FromStorageKey(settings.RtaDashboardOtherSideKey), tap);
            settings.RtaDashboardSelectionKey = next.Active.StorageKey;
            settings.RtaDashboardOtherSideKey = next.Remembered?.StorageKey ?? "";
            settings.Save();
        }
        else
        {
            (_rtaPageSelection, _rtaPageOtherSide) = RtaChannelSelection.SwitchingSides(_rtaPageSelection, _rtaPageOtherSide, tap);
        }
        RtaSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The main window opened a page: -1 for the dashboard, else the
    /// channel id. A channel page starts on its own channel, or on nothing if
    /// the user hid the spectrum on channel pages.</summary>
    public void SetRtaPage(int channelId)
    {
        if (channelId == _rtaPageChannelId) return;
        _rtaPageChannelId = channelId;
        if (channelId >= 0 && RtaChannelOf(channelId) is var (tap, channel))
        {
            _rtaPageOtherSide = null;
            _rtaPageSelection = new RtaChannelSelection(tap,
                AppSettings.Instance.RtaChannelPagesShowSpectrum ? new[] { channel } : Array.Empty<int>());
        }
        RtaSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where the pages show their spectrum, or another analyser
    /// preference, changed: everything showing it re-reads its settings.</summary>
    public void RtaPreferencesChanged()
    {
        AppSettings.Instance.Save();
        Rta.SetOptions(RtaOptionsFromSettings);
        RtaSelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
