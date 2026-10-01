using DSPiConsole.Core.GraphEditing;
using DSPiConsole.Core.Models;

namespace DSPiConsole.ViewModels;

/// <summary>
/// The view model as host of the on-graph PEQ editor (<see cref="PeqGraphEditor"/>).
/// A drag never touches the model: the editor sends live values straight to the
/// device (about 30 a second, through the throttled sender below), the band list
/// follows through <see cref="GraphBandLive"/>, and the model is written once,
/// on release, through <see cref="CommitGraphBands"/>. Everything is mirrored
/// onto the other half of a linked input pair, as the band list does.
/// </summary>
public partial class MainViewModel : IPeqEditorHost
{
    /// <summary>Graph band selection and hover, shared by the main graph, the
    /// pop-out graph and the band list.</summary>
    public PeqGraphSelection PeqSelection { get; } = new();

    PeqGraphSelection IPeqEditorHost.Selection => PeqSelection;

    /// <summary>(channel, band, value): a band's live value during a graph
    /// edit, for the band list to show without a rebuild.</summary>
    public event Action<int, int, FilterParams>? GraphBandLive;

    /// <summary>A graph edit's live display has ended for a channel.</summary>
    public event Action<int>? GraphLiveEnded;

    public void CommitGraphBands(int channel, IReadOnlyList<(int Band, FilterParams Params)> changes)
    {
        if (changes.Count == 0) return;
        CancelLiveFilterSends();
        int partner = IsInputPairLinked(channel) ? GetLinkedInputChannel(channel) : -1;
        var writes = new List<(int Channel, int Band, FilterParams Params)>();
        foreach (var (band, p) in changes)
        {
            SetModelBand(channel, band, p.Clone());
            writes.Add((channel, band, p.Clone()));
            if (partner >= 0)
            {
                SetModelBand(partner, band, p.Clone());
                writes.Add((partner, band, p.Clone()));
            }
        }
        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
        Task.Run(() =>
        {
            lock (_filterWriteGate)
                foreach (var (ch, band, p) in writes)
                    try { _device.SetFilter(ch, band, p); } catch { }
        });
    }

    public void SendGraphBandsToDevice(int channel, IReadOnlyList<(int Band, FilterParams Params)> changes)
    {
        int partner = IsInputPairLinked(channel) ? GetLinkedInputChannel(channel) : -1;
        var writes = new List<(int Channel, int WireBand, FilterParams Params)>();
        foreach (var (band, p) in changes)
        {
            writes.Add((channel, band, p.Clone()));
            if (partner >= 0) writes.Add((partner, band, p.Clone()));
        }
        QueueLiveSend(writes);
    }

    public void SetGraphBandBypass(int channel, IReadOnlyList<int> bands, bool bypass)
    {
        if (bands.Count == 0) return;
        CancelLiveFilterSends();
        var targets = new List<int> { channel };
        if (IsInputPairLinked(channel)) targets.Add(GetLinkedInputChannel(channel));
        foreach (int ch in targets)
            if (_channelData.TryGetValue(ch, out var filters))
                foreach (int band in bands)
                    if (band < filters.Count) filters[band].Bypass = bypass;
        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
        var list = bands.ToList();
        Task.Run(() =>
        {
            lock (_filterWriteGate)
                foreach (int ch in targets)
                    foreach (int band in list)
                        try { _device.SetBandBypass(ch, band, bypass); } catch { }
        });
    }

    public void ShowLive(int channel, int band, FilterParams p) => GraphBandLive?.Invoke(channel, band, p);

    public void EndLive(int channel) => GraphLiveEnded?.Invoke(channel);

    private void SetModelBand(int channel, int band, FilterParams p)
    {
        if (_channelData.TryGetValue(channel, out var filters) && band < filters.Count)
            filters[band] = p;
    }

    // ── Throttled live sender ───────────────────────────────────────────────
    // Graph drags would write at pointer rate, which the bus cannot take. Live
    // writes are merged by (channel, band) — a superseded value is stale by the
    // time it could reach the wire — and sent at most every LiveSendIntervalMs.
    // Every filter write, live or authoritative, holds _filterWriteGate, and an
    // authoritative write bumps _liveGeneration first, so a live write queued
    // before it can never land after it.

    private const int LiveSendIntervalMs = 33;
    private readonly object _filterWriteGate = new();
    private readonly object _liveSendLock = new();
    private readonly Dictionary<(int Channel, int WireBand), FilterParams> _livePending = new();
    private bool _liveSendRunning;
    private int _liveGeneration;

    private void QueueLiveSend(List<(int Channel, int WireBand, FilterParams Params)> writes)
    {
        int generation;
        lock (_liveSendLock)
        {
            foreach (var (ch, band, p) in writes) _livePending[(ch, band)] = p;
            if (_liveSendRunning) return;   // the running loop will pick these up
            _liveSendRunning = true;
            generation = _liveGeneration;
        }

        Task.Run(async () =>
        {
            while (true)
            {
                List<KeyValuePair<(int Channel, int WireBand), FilterParams>> batch;
                lock (_liveSendLock)
                {
                    // A cancel already released the running flag (and a newer
                    // loop may hold it now), so a superseded loop just stops.
                    if (generation != _liveGeneration) return;
                    if (_livePending.Count == 0)
                    {
                        _liveSendRunning = false;
                        return;
                    }
                    batch = _livePending.ToList();
                    _livePending.Clear();
                }

                lock (_filterWriteGate)
                {
                    foreach (var ((ch, band), p) in batch)
                    {
                        if (Volatile.Read(ref _liveGeneration) != generation) break;
                        // A dropped live frame is superseded by the next one anyway.
                        try { _device.SetFilter(ch, band, p); } catch { }
                    }
                }

                await Task.Delay(LiveSendIntervalMs);
            }
        });
    }

    /// <summary>
    /// Discards any live value still waiting to be sent. Called before every
    /// authoritative filter write so a throttled write queued mid-drag can't
    /// land after it and re-apply a stale value.
    /// </summary>
    public void CancelLiveFilterSends()
    {
        lock (_liveSendLock)
        {
            _livePending.Clear();
            _liveGeneration++;
            _liveSendRunning = false;
        }
    }
}
