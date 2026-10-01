using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Output limiter (firmware wire V32) state and device orchestration (0x81).
/// Every output has its own enable, threshold, release and link group; there is
/// no master switch. Outputs sharing a link group are ganged by the firmware:
/// one SET reaches the whole group, and an output joining a group adopts its
/// settings. <see cref="LimiterGang"/> mirrors that, because the app's own
/// writes are never echoed back to it.
/// <para>
/// The settings follow output_config_mode like the output pins: with-preset
/// they are part of the preset, independent they are device-wide and saved by
/// Save Output Config. So they live in the snapshot's IO block.
/// </para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>The feature is present (wire V32+).</summary>
    [ObservableProperty]
    private bool _limiterSupported;

    private readonly LimiterOutputSettings[] _limiter = Enumerable.Range(0, BulkParamsParser.WireMaxOutputChannels)
        .Select(_ => new LimiterOutputSettings()).ToArray();

    /// <summary>One entry per wire output slot (9), so any index from the bulk
    /// image or a preset file is valid on either platform; only the platform's
    /// outputs are ever shown or written.</summary>
    public IReadOnlyList<LimiterOutputSettings> LimiterOutputs => _limiter;

    /// <summary>Raised on the UI thread whenever any output's limiter changes.</summary>
    public event EventHandler? LimiterChanged;

    /// <summary>True when any output's limiter is on, which adds the 32-sample
    /// lookahead to every output.</summary>
    public bool AnyLimiterEnabled => _limiter.Take(LimiterOutputCount).Any(o => o.Enabled);

    private int LimiterOutputCount => Math.Min(_device.NumOutputChannels, _limiter.Length);

    private bool IsLimiterOutput(int output) => output >= 0 && output < LimiterOutputCount;

    /// <summary>Puts the limiter back to the last saved baseline, for Discard
    /// in independent mode. True when anything had changed.</summary>
    internal bool RevertLimiterTo(IReadOnlyList<LimiterOutputSettings> saved)
    {
        if (!LimiterSupported || saved.Count == 0) return false;
        var targets = new Dictionary<int, LimiterOutputSettings>();
        for (int k = 0; k < Math.Min(saved.Count, LimiterOutputCount); k++)
            if (_limiter[k] != saved[k]) targets[k] = saved[k];
        if (targets.Count == 0) return false;
        // Every output takes part, so a group re-forms around its saved leader.
        for (int k = 0; k < Math.Min(saved.Count, LimiterOutputCount); k++) targets[k] = saved[k];
        ApplyLimiterSettings(targets);
        return true;
    }

    private void LimiterEdited()
    {
        LimiterStateChanged();
        CheckDirty();
    }

    private void LimiterStateChanged()
    {
        _anyLimiterEnabled = AnyLimiterEnabled;
        LimiterChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(AnyLimiterEnabled));
    }

    /// <summary>A threshold or release during a drag: device only, clamped as
    /// the firmware does. The setter commits it on release.</summary>
    public void SendLimiterParamLive(int output, byte index, float value)
    {
        if (!IsLimiterOutput(output) || float.IsNaN(value)) return;
        float v = index == LimiterParam.ReleaseMs ? LimiterLimits.ClampRelease(value) : LimiterLimits.ClampThreshold(value);
        DeviceWrite(() => _device.SetLimiterParam(output, index, v));
    }

    // ── Gain-reduction meter ──
    // Read on the 60 ms status poll, but only while a limiter icon is on screen
    // (WatchLimiterMeter) and some limiter is on; otherwise nothing can be
    // limiting, and the reading is cleared once rather than polled.

    private int _limiterWatchers;
    private volatile bool _anyLimiterEnabled;
    private float[] _limiterReductionDb = Array.Empty<float>();

    /// <summary>Gain reduction per output in dB (0 = none) from the last poll;
    /// empty when not read.</summary>
    public IReadOnlyList<float> LimiterReductionDb => _limiterReductionDb;

    /// <summary>Raised on the UI thread when the reduction readings change.</summary>
    public event EventHandler? LimiterMeterChanged;

    /// <summary>A view showing live limiter state starts (true) or stops
    /// (false) wanting the meter read.</summary>
    public void WatchLimiterMeter(bool watching) =>
        Interlocked.Add(ref _limiterWatchers, watching ? 1 : -1);

    /// <summary>From the status poll thread.</summary>
    private void PollLimiterMeter()
    {
        if (!LimiterSupported || Volatile.Read(ref _limiterWatchers) <= 0) return;
        float[] reading;
        if (_anyLimiterEnabled)
        {
            if (_device.GetLimiterMeter(_device.NumOutputChannels) is not { } gr) return;
            reading = gr;
        }
        else
        {
            if (_limiterReductionDb.Length == 0) return;
            reading = Array.Empty<float>();
        }
        if (reading.AsSpan().SequenceEqual(_limiterReductionDb)) return;
        _dispatcher.TryEnqueue(() =>
        {
            _limiterReductionDb = reading;
            LimiterMeterChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void SetLimiterEnabled(int output, bool enabled)
    {
        if (!IsLimiterOutput(output)) return;
        LimiterGang.Edit(output, _limiter, LimiterOutputCount, o => o with { Enabled = enabled });
        DeviceWrite(() => _device.SetLimiterParam(output, LimiterParam.Enabled, enabled ? 1 : 0));
        LimiterEdited();
    }

    public void SetLimiterThreshold(int output, float db)
    {
        if (!IsLimiterOutput(output) || float.IsNaN(db)) return;
        float v = LimiterLimits.ClampThreshold(db);
        LimiterGang.Edit(output, _limiter, LimiterOutputCount, o => o with { ThresholdDb = v });
        DeviceWrite(() => _device.SetLimiterParam(output, LimiterParam.ThresholdDb, v));
        LimiterEdited();
    }

    public void SetLimiterRelease(int output, float ms)
    {
        if (!IsLimiterOutput(output) || float.IsNaN(ms)) return;
        float v = LimiterLimits.ClampRelease(ms);
        LimiterGang.Edit(output, _limiter, LimiterOutputCount, o => o with { ReleaseMs = v });
        DeviceWrite(() => _device.SetLimiterParam(output, LimiterParam.ReleaseMs, v));
        LimiterEdited();
    }

    /// <summary>Joining a group adopts the settings of its lowest-numbered
    /// existing member; the first member keeps its own, leaving keeps the
    /// current ones.</summary>
    public void SetLimiterLinkGroup(int output, int group)
    {
        if (!IsLimiterOutput(output)) return;
        int g = LimiterLimits.ClampLinkGroup(group);
        LimiterGang.SetGroup(output, g, _limiter, LimiterOutputCount);
        DeviceWrite(() => _device.SetLimiterParam(output, LimiterParam.LinkGroup, g));
        LimiterEdited();
    }

    /// <summary>
    /// Puts outputs back to exact per-output settings, for Revert and preset
    /// file import. Ganging would scramble a plain write-each-field pass (a
    /// threshold written to an output still in its old group spreads to that
    /// group), so the targets leave their groups first, take their own values
    /// unlinked, then rejoin in ascending order, which makes each group's
    /// lowest member its leader as the firmware does. Enable goes last so a
    /// limiter engages on its new threshold.
    /// </summary>
    public void ApplyLimiterSettings(IReadOnlyDictionary<int, LimiterOutputSettings> targets)
    {
        var outputs = targets.Keys.Where(IsLimiterOutput).OrderBy(k => k).ToList();
        foreach (int k in outputs.Where(k => _limiter[k].LinkGroup != 0))
            SetLimiterLinkGroup(k, 0);
        foreach (int k in outputs)
        {
            var t = targets[k];
            SetLimiterThreshold(k, t.ThresholdDb);
            SetLimiterRelease(k, t.ReleaseMs);
            SetLimiterEnabled(k, t.Enabled);
        }
        foreach (int k in outputs.Where(k => targets[k].LinkGroup != 0))
            SetLimiterLinkGroup(k, targets[k].LinkGroup);
    }

    /// <summary>Copies one output's enable, threshold and release to every
    /// output with one all-outputs SET each. The link group is left alone:
    /// copying it would link every output into one group.</summary>
    public void CopyLimiterToAllOutputs(int from)
    {
        if (!IsLimiterOutput(from)) return;
        var src = _limiter[from];
        for (int k = 0; k < LimiterOutputCount; k++)
            _limiter[k] = _limiter[k] with { Enabled = src.Enabled, ThresholdDb = src.ThresholdDb, ReleaseMs = src.ReleaseMs };
        DeviceWrite(() =>
        {
            _device.SetLimiterParam(LimiterParam.AllOutputs, LimiterParam.ThresholdDb, src.ThresholdDb);
            _device.SetLimiterParam(LimiterParam.AllOutputs, LimiterParam.ReleaseMs, src.ReleaseMs);
            _device.SetLimiterParam(LimiterParam.AllOutputs, LimiterParam.Enabled, src.Enabled ? 1 : 0);
        });
        LimiterEdited();
    }

    /// <summary>Sets every output's link group, one SET each in ascending order,
    /// so each group's lowest output keeps its settings and the rest adopt them.
    /// Outputs past the end of <paramref name="groups"/> are unlinked.</summary>
    public void SetLimiterLinkGroups(IReadOnlyList<int> groups)
    {
        for (int k = 0; k < LimiterOutputCount; k++)
            SetLimiterLinkGroup(k, k < groups.Count ? groups[k] : 0);
    }

    /// <summary>Switches every output's limiter on or off with one SET.</summary>
    public void SetLimiterEnabledOnAll(bool enabled)
    {
        if (!LimiterSupported) return;
        for (int k = 0; k < LimiterOutputCount; k++) _limiter[k] = _limiter[k] with { Enabled = enabled };
        DeviceWrite(() => _device.SetLimiterParam(LimiterParam.AllOutputs, LimiterParam.Enabled, enabled ? 1 : 0));
        LimiterEdited();
    }

    /// <summary>Seed limiter state from a bulk read. The firmware stores raw
    /// values, so an older preset's group can disagree until the next
    /// recompute; gang them the way it will.</summary>
    internal void SeedLimiterFromBulk(BulkParams bp)
    {
        LimiterSupported = bp.HasLimiter;
        if (!LimiterSupported) return;
        for (int k = 0; k < _limiter.Length && k < bp.Limiter.Length; k++)
            _limiter[k] = SanitizeLimiter(bp.Limiter[k]);
        LimiterGang.GangAll(_limiter, Math.Min(bp.NumOutputChannels, _limiter.Length));
        LimiterStateChanged();
    }

    /// <summary>The audio path's own rule for stored values: NaN takes the
    /// default, then values clamp.</summary>
    private static LimiterOutputSettings SanitizeLimiter(LimiterOutputSettings s) => s with
    {
        ThresholdDb = float.IsNaN(s.ThresholdDb) ? LimiterLimits.DefaultThresholdDb : LimiterLimits.ClampThreshold(s.ThresholdDb),
        ReleaseMs = float.IsNaN(s.ReleaseMs) ? LimiterLimits.DefaultReleaseMs : LimiterLimits.ClampRelease(s.ReleaseMs),
        LinkGroup = LimiterLimits.ClampLinkGroup(s.LinkGroup),
    };

    /// <summary>Re-read every output's limiter from the device. Blocking: call
    /// off the UI thread. Clears <see cref="LimiterSupported"/> if the status
    /// probe STALLs.</summary>
    public void FetchLimiter()
    {
        if (_device.GetLimiterStatus() is not { } status)
        {
            _dispatcher.TryEnqueue(() => LimiterSupported = false);
            return;
        }
        int count = Math.Min(status.Outputs, _limiter.Length);
        var read = new LimiterOutputSettings?[count];
        for (int k = 0; k < count; k++)
        {
            var en = _device.GetLimiterParam(k, LimiterParam.Enabled);
            var th = _device.GetLimiterParam(k, LimiterParam.ThresholdDb);
            var rel = _device.GetLimiterParam(k, LimiterParam.ReleaseMs);
            var grp = _device.GetLimiterParam(k, LimiterParam.LinkGroup);
            if (en == null || th == null || rel == null || grp == null) continue;
            read[k] = new LimiterOutputSettings
            {
                Enabled = en.Value != 0,
                ThresholdDb = th.Value,
                ReleaseMs = rel.Value,
                LinkGroup = LimiterLimits.ClampLinkGroup((int)Math.Round(grp.Value)),
            };
        }
        _dispatcher.TryEnqueue(() =>
        {
            LimiterSupported = true;
            for (int k = 0; k < count; k++)
                if (read[k] is { } r) _limiter[k] = r;
            LimiterEdited();
        });
    }
}
